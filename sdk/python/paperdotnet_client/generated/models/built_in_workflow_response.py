from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .json_object import JsonObject

@dataclass
class BuiltInWorkflowResponse(AdditionalDataHolder, Parsable):
    """
    A built-in workflow (EVT-12) and its state in the workspace: its `parameters` (JSON Schema), whether the serverhas what it needs (`available`), and when it was turned on, the `workflowId` it runs as and its `values`.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The available property
    available: Optional[bool] = None
    # The description property
    description: Optional[str] = None
    # The enabled property
    enabled: Optional[bool] = None
    # The key property
    key: Optional[str] = None
    # The name property
    name: Optional[str] = None
    # The parameters property
    parameters: Optional[JsonObject] = None
    # The requires property
    requires: Optional[str] = None
    # The values property
    values: Optional[JsonObject] = None
    # The workflowId property
    workflow_id: Optional[UUID] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> BuiltInWorkflowResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: BuiltInWorkflowResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return BuiltInWorkflowResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .json_object import JsonObject

        from .json_object import JsonObject

        fields: dict[str, Callable[[Any], None]] = {
            "available": lambda n : setattr(self, 'available', n.get_bool_value()),
            "description": lambda n : setattr(self, 'description', n.get_str_value()),
            "enabled": lambda n : setattr(self, 'enabled', n.get_bool_value()),
            "key": lambda n : setattr(self, 'key', n.get_str_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
            "parameters": lambda n : setattr(self, 'parameters', n.get_object_value(JsonObject)),
            "requires": lambda n : setattr(self, 'requires', n.get_str_value()),
            "values": lambda n : setattr(self, 'values', n.get_object_value(JsonObject)),
            "workflowId": lambda n : setattr(self, 'workflow_id', n.get_uuid_value()),
        }
        return fields
    
    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_bool_value("available", self.available)
        writer.write_str_value("description", self.description)
        writer.write_bool_value("enabled", self.enabled)
        writer.write_str_value("key", self.key)
        writer.write_str_value("name", self.name)
        writer.write_object_value("parameters", self.parameters)
        writer.write_str_value("requires", self.requires)
        writer.write_object_value("values", self.values)
        writer.write_uuid_value("workflowId", self.workflow_id)
        writer.write_additional_data_value(self.additional_data)
    


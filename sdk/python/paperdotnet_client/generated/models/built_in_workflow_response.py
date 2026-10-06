from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .built_in_scope import BuiltInScope
    from .json_object import JsonObject

@dataclass
class BuiltInWorkflowResponse(AdditionalDataHolder, Parsable):
    """
    A built-in workflow (EVT-12) and its state in the workspace: its `parameters` (JSON Schema), whether the serverhas what it needs (`available`), and when it was turned on, the `workflowId` it runs as and its `values`.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The allowManualLaunch property
    allow_manual_launch: Optional[bool] = False
    # The enabledByDefault property
    enabled_by_default: Optional[bool] = False
    # The required property
    required: Optional[bool] = False
    from .built_in_scope import BuiltInScope

    # The scope property
    scope: Optional[BuiltInScope] = BuiltInScope("workspace")
    # The system property
    system: Optional[bool] = False
    # The available property
    available: Optional[bool] = None
    # The description property
    description: Optional[str] = None
    # The enabled property
    enabled: Optional[bool] = None
    # The inputSchema property
    input_schema: Optional[JsonObject] = None
    # The key property
    key: Optional[str] = None
    # The name property
    name: Optional[str] = None
    # Once it was turned on in the workspace: the ETag for `If-Match` on changes (the workflow's; the same as the`ETag` header).
    odata_etag: Optional[str] = None
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
        from .built_in_scope import BuiltInScope
        from .json_object import JsonObject

        from .built_in_scope import BuiltInScope
        from .json_object import JsonObject

        fields: dict[str, Callable[[Any], None]] = {
            "allowManualLaunch": lambda n : setattr(self, 'allow_manual_launch', n.get_bool_value()),
            "available": lambda n : setattr(self, 'available', n.get_bool_value()),
            "description": lambda n : setattr(self, 'description', n.get_str_value()),
            "enabled": lambda n : setattr(self, 'enabled', n.get_bool_value()),
            "enabledByDefault": lambda n : setattr(self, 'enabled_by_default', n.get_bool_value()),
            "inputSchema": lambda n : setattr(self, 'input_schema', n.get_object_value(JsonObject)),
            "key": lambda n : setattr(self, 'key', n.get_str_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
            "@odata.etag": lambda n : setattr(self, 'odata_etag', n.get_str_value()),
            "parameters": lambda n : setattr(self, 'parameters', n.get_object_value(JsonObject)),
            "required": lambda n : setattr(self, 'required', n.get_bool_value()),
            "requires": lambda n : setattr(self, 'requires', n.get_str_value()),
            "scope": lambda n : setattr(self, 'scope', n.get_enum_value(BuiltInScope)),
            "system": lambda n : setattr(self, 'system', n.get_bool_value()),
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
        writer.write_bool_value("allowManualLaunch", self.allow_manual_launch)
        writer.write_bool_value("available", self.available)
        writer.write_str_value("description", self.description)
        writer.write_bool_value("enabled", self.enabled)
        writer.write_bool_value("enabledByDefault", self.enabled_by_default)
        writer.write_object_value("inputSchema", self.input_schema)
        writer.write_str_value("key", self.key)
        writer.write_str_value("name", self.name)
        writer.write_str_value("@odata.etag", self.odata_etag)
        writer.write_object_value("parameters", self.parameters)
        writer.write_bool_value("required", self.required)
        writer.write_str_value("requires", self.requires)
        writer.write_enum_value("scope", self.scope)
        writer.write_bool_value("system", self.system)
        writer.write_object_value("values", self.values)
        writer.write_uuid_value("workflowId", self.workflow_id)
        writer.write_additional_data_value(self.additional_data)
    


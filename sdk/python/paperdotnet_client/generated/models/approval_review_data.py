from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .json_object import JsonObject

@dataclass
class ApprovalReviewData(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The canDecide property
    can_decide: Optional[bool] = None
    # The data property
    data: Optional[JsonObject] = None
    # The reason property
    reason: Optional[str] = None
    # The renderer property
    renderer: Optional[str] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ApprovalReviewData:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ApprovalReviewData
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ApprovalReviewData()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .json_object import JsonObject

        from .json_object import JsonObject

        fields: dict[str, Callable[[Any], None]] = {
            "canDecide": lambda n : setattr(self, 'can_decide', n.get_bool_value()),
            "data": lambda n : setattr(self, 'data', n.get_object_value(JsonObject)),
            "reason": lambda n : setattr(self, 'reason', n.get_str_value()),
            "renderer": lambda n : setattr(self, 'renderer', n.get_str_value()),
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
        writer.write_bool_value("canDecide", self.can_decide)
        writer.write_object_value("data", self.data)
        writer.write_str_value("reason", self.reason)
        writer.write_str_value("renderer", self.renderer)
        writer.write_additional_data_value(self.additional_data)
    

